using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Interfaces;
using AttendanceSystem.Models.Exceptions;
using AttendanceSystem.Helpers;

namespace AttendanceSystem.Services.Implementations;

/// <summary>
/// 考勤核心服务：打卡、考勤记录查询、月度汇总等。
/// 打卡时会根据员工的考勤组/班次，实时算出迟到、早退、加班、实际工时和考勤状态。
/// 代码按主题拆在同目录的 AttendanceService.*.cs 里（Punch / Records / Reports / Summary / Stats / Adjustments / Backfill / WorkHours / Helpers），同一个类。
/// </summary>
public partial class AttendanceService(AttendanceDbContext db, IOptions<AppSettingsOptions> appOptions, ILogger<AttendanceService> logger, TimeProvider? timeProvider = null) : IAttendanceService
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    private const int MaxPunchAttempts = 5;
}
