using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;

namespace AttendanceSystem.Services.Interfaces;

/// <summary>考勤业务服务契约。</summary>
public interface IAttendanceService
{
    /// <summary>打卡（上班/下班），返回打卡结果与计算出的考勤状态。</summary>
    Task<PunchResponseDto>         PunchAsync(int userId, PunchRequestDto request);
    /// <summary>获取某员工今日考勤记录。</summary>
    Task<AttendanceRecordDto?>     GetTodayAttendanceAsync(int userId);
    /// <summary>按条件查询个人考勤记录列表。</summary>
    Task<List<AttendanceRecordDto>> GetPersonalAttendanceAsync(PersonalAttendanceQueryDto query);
    /// <summary>按部门/考勤组查询考勤记录列表。</summary>
    Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto query);
    /// <summary>获取某员工指定月份的汇总（含每日明细）。</summary>
    Task<MonthlySummaryDto?>       GetMonthlySummaryAsync(int userId, int year, int month);
    /// <summary>获取某员工指定月份的排班安排（“我的排班”页用，方便员工自己查看上班时间）。</summary>
    Task<List<MyScheduleDto>>      GetMyScheduleAsync(int userId, int year, int month);
    /// <summary>获取部门/考勤组指定月份的汇总列表。</summary>
    Task<List<MonthlySummaryDto>>  GetDeptMonthlySummariesAsync(int? deptId, int? groupId, int year, int month);
    /// <summary>生成/重算指定月份所有在职员工的考勤汇总。</summary>
    Task                           GenerateMonthlySummaryAsync(int year, int month);
    /// <summary>今日考勤看板统计（出勤、缺勤、迟到等）。</summary>
    Task<AttendanceStatsDto>       GetTodayStatsAsync(int? groupId = null);
    /// <summary>
    /// 看板下钻：某统计类别（total/present/absent/late/onleave/notpunched）对应的具体人员名单。
    /// 分类口径与 <see cref="GetTodayStatsAsync"/> 完全一致，保证卡片数字和点开的名单条数对得上。
    /// </summary>
    Task<List<AttendanceRecordDto>> GetTodayStatsDetailAsync(string category, int? groupId = null);
    /// <summary>判断指定日期是否为节假日（排除调班补班日）。</summary>
    Task<bool>                     IsHolidayAsync(DateOnly date, int? groupId = null);
    /// <summary>获取指定月份的假期信息列表（法定节假日/公司休息日/调班补班日），供日历页标注非工作日用。</summary>
    Task<List<HolidayInfoDto>>     GetMonthHolidaysAsync(int year, int month, int? groupId);
    /// <summary>获取某员工某天的排班。</summary>
    Task<ShiftAssignment?>         GetShiftAssignmentAsync(int userId, DateOnly date);
    /// <summary>审批通过后回写对应考勤记录（补卡/请假）。</summary>
    Task                           UpdateAttendanceAfterApprovalAsync(int approvalRequestId);
}
