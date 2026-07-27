using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>打卡页：显示今天的考勤状态，处理上/下班打卡（带定位）。</summary>
[Authorize]
public class PunchCardModel : AppPageModel
{
    public AttendanceRecordDto? TodayRecord { get; set; }   // 今天的考勤记录（用于页面显示）
    public string  Message     { get; set; } = string.Empty;
    public bool    IsSuccess   { get; set; }
    public bool    ShowMessage { get; set; }

    // 浏览器定位拿到的经纬度，会随表单提交上来
    [BindProperty] public double? Latitude  { get; set; }
    [BindProperty] public double? Longitude { get; set; }

    // 本系统的手动打卡功能已关闭：改成统一只认钉钉人脸打卡的数据，避免两边数据对不上。
    // GET/POST 都直接跳到"我的记录"，不再显示这个页面。
    public IActionResult OnGetAsync() => RedirectToPage("/Attendance/MyRecord");

    public IActionResult OnPostPunchAsync(string punchType) => RedirectToPage("/Attendance/MyRecord");
}
