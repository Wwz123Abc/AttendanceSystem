using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.DTOs;

/// <summary>打卡结果：后台处理完打卡后，返回给网页的结果。</summary>
public class PunchResponseDto
{
    public bool             Success    { get; set; }                  // 是否成功
    public string           Message    { get; set; } = string.Empty;  // 提示文字
    public DateTime?        PunchTime  { get; set; }                  // 实际打卡时间
    public AttendanceStatus Status     { get; set; }                  // 算出来的考勤状态
    public string?          StatusText { get; set; }                  // 状态中文名
    public int?             LateMinutes { get; set; }                 // 迟到分钟数（不迟到为空）
}
