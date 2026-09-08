namespace AttendanceSystem.Models.Options;

/// <summary>
/// 应用通用配置（绑定 appsettings.json 的 "AppSettings" 节）。
/// </summary>
public class AppSettingsOptions
{
    public const string SectionName = "AppSettings";

    /// <summary>登录有效期（小时）。</summary>
    public int TokenExpireHours { get; set; } = 8;

    /// <summary>连续登录失败几次就临时锁定账号。</summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>触发锁定后，锁定多少分钟（过了自动解锁，不需要人工处理）。</summary>
    public int LoginLockoutMinutes { get; set; } = 15;

    /// <summary>上传文件根目录（相对 wwwroot）。</summary>
    public string UploadPath { get; set; } = "uploads";

    /// <summary>
    /// 出差全勤的默认标准工时（小时）：出差期间不用打卡，按“全勤”记工时（工资按工时结算）。
    /// 如果那天有排班，优先用班次自己的标准工时；没有排班就用这个默认值。
    /// </summary>
    public decimal DefaultDailyWorkHours { get; set; } = 8;
}
