using System.ComponentModel.DataAnnotations.Schema;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Models.Entities;

/// <summary>午间打卡"一段窗口"的判定结果：这段要求的时间范围，以及当天有没有打上（打上了是几点）。</summary>
public readonly record struct MidCheckWindowResult(TimeOnly WindowStart, TimeOnly WindowEnd, TimeOnly? HitTime)
{
    public bool IsSatisfied => HitTime is not null;
}
