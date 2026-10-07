namespace AttendanceSystem.Models.Enums;

/// <summary>假期类型：这一天属于哪种特殊日子。</summary>
public enum HolidayType
{
    LegalHoliday        = 1,  // 法定节假日（如国庆），不用上班
    CompanyRestDay      = 2,  // 公司自定的休息日，不用上班
    CompensatoryWorkDay = 3   // 调班补班日：原本是周末，但要上班
}
