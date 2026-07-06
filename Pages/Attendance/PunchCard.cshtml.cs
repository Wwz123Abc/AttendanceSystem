using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Pages.Attendance;

/// <summary>打卡页：显示今天的考勤状态，处理上/下班打卡（带定位）。</summary>
[Authorize]
public class PunchCardModel(IAttendanceService attendanceService) : AppPageModel
{
    public AttendanceRecordDto? TodayRecord { get; set; }   // 今天的考勤记录（用于页面显示）
    public string  Message     { get; set; } = string.Empty;
    public bool    IsSuccess   { get; set; }
    public bool    ShowMessage { get; set; }

    // 浏览器定位拿到的经纬度，会随表单提交上来
    [BindProperty] public double? Latitude  { get; set; }
    [BindProperty] public double? Longitude { get; set; }

    /// <summary>打开页面时，加载今天的考勤状态。</summary>
    public async Task OnGetAsync()
        => TodayRecord = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);

    /// <summary>点“上班打卡/下班打卡”按钮时执行。</summary>
    public async Task<IActionResult> OnPostPunchAsync(string punchType)
    {
        // 把传来的文字("ClockIn"/"ClockOut")转成枚举；转不了就报错
        if (!Enum.TryParse<PunchType>(punchType, out var pt))
        {
            TodayRecord = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);
            Message = "无效的打卡类型"; ShowMessage = true; IsSuccess = false;
            return Page();
        }

        // 调考勤服务执行打卡（DeviceInfo 取浏览器标识，最多留 200 字）
        var result = await attendanceService.PunchAsync(CurrentUserId, new PunchRequestDto
        {
            PunchType = pt,
            Latitude  = Latitude,
            Longitude = Longitude,
            DeviceInfo = Request.Headers.UserAgent.ToString().Length > 200
                ? Request.Headers.UserAgent.ToString()[..200]
                : Request.Headers.UserAgent.ToString()
        });

        // 刷新今天的状态，并把结果提示显示出来
        TodayRecord = await attendanceService.GetTodayAttendanceAsync(CurrentUserId);
        Message = result.Message; ShowMessage = true; IsSuccess = result.Success;
        return Page();
    }
}
