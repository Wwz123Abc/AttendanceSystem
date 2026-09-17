using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Services.Interfaces;

namespace AttendanceSystem.Tests;

/// <summary>
/// 给 ApprovalService 的并发/事务测试用的假考勤服务：ApprovalService 只会调用
/// <see cref="UpdateAttendanceAfterApprovalAsync"/> 这一个方法，这里记下每次调用的 approvalRequestId，
/// 方便测试断言"考勤有没有被回写、回写了几次"；其余方法测试用不到，直接抛异常，
/// 一旦哪天 ApprovalService 改动后意外调用了其他方法，测试会立刻报错而不是悄悄通过。
/// </summary>
public class FakeAttendanceService : IAttendanceService
{
    public List<int> UpdatedApprovalRequestIds { get; } = [];

    public Task UpdateAttendanceAfterApprovalAsync(int approvalRequestId)
    {
        UpdatedApprovalRequestIds.Add(approvalRequestId);
        return Task.CompletedTask;
    }

    public Task<PunchResponseDto> PunchAsync(int userId, PunchRequestDto request, bool skipLocationCheck = false) => throw new NotImplementedException();
    public Task<(bool Valid, string? Message)> ValidateLocationAsync(int? attendanceGroupId, double? latitude, double? longitude, double? accuracyMeters = null) => throw new NotImplementedException();
    public Task<AttendanceRecordDto?> GetTodayAttendanceAsync(int userId) => throw new NotImplementedException();
    public Task<List<AttendanceRecordDto>> GetPersonalAttendanceAsync(PersonalAttendanceQueryDto query) => throw new NotImplementedException();
    public Task<List<AttendanceRecordDto>> GetDeptAttendanceAsync(DeptAttendanceQueryDto query, HashSet<int>? deptIds = null) => throw new NotImplementedException();
    public Task<MonthlySummaryDto?> GetMonthlySummaryAsync(int userId, int year, int month) => throw new NotImplementedException();
    public Task<List<MyScheduleDto>> GetMyScheduleAsync(int userId, int year, int month) => throw new NotImplementedException();
    public Task<List<MonthlySummaryDto>> GetDeptMonthlySummariesAsync(int? deptId, int? groupId, int year, int month, HashSet<int>? scopeDeptIds = null) => throw new NotImplementedException();
    public Task GenerateMonthlySummaryAsync(int year, int month, IReadOnlyCollection<int>? onlyUserIds = null) => throw new NotImplementedException();
    public Task<AttendanceStatsDto> GetTodayStatsAsync(int? groupId = null, HashSet<int>? deptIds = null) => throw new NotImplementedException();
    public Task<List<AttendanceRecordDto>> GetTodayStatsDetailAsync(string category, int? groupId = null, HashSet<int>? deptIds = null) => throw new NotImplementedException();
    public Task<bool> IsHolidayAsync(DateOnly date, int? groupId = null) => throw new NotImplementedException();
    public Task<List<HolidayInfoDto>> GetMonthHolidaysAsync(int year, int month, int? groupId) => throw new NotImplementedException();
    public Task<ShiftAssignment?> GetShiftAssignmentAsync(int userId, DateOnly date) => throw new NotImplementedException();
    public Task AdminAdjustPunchAsync(int userId, DateOnly workDate, DateTime? clockIn, DateTime? clockOut, string? remark, string? operatorName) => throw new NotImplementedException();
    public Task<TemplateReportResultDto> GenerateTemplateReportAsync(DateOnly start, DateOnly end, List<int>? deptIds) => throw new NotImplementedException();
    public Task<List<AttendanceRecordDto>> GetClockTimeSheetAsync(DateOnly start, DateOnly end, List<int>? deptIds) => throw new NotImplementedException();
}
