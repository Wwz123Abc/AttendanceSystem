namespace AttendanceSystem.Models.DTOs;

/// <summary>管理看板"各考勤组 / 各班次出勤率"里的一行（今天的数字，口径同今日出勤率卡片）。</summary>
public class RateBreakdownRowDto
{
    /// <summary>考勤组名，或"考勤组 · 班次名（上下班时间）"；没分组 / 没排班的人归在"未分组""未排班"里。</summary>
    public string Name { get; set; } = "";

    public int Total { get; set; }        // 这一行一共多少人（在职、需要考勤）
    public int Expected { get; set; }     // 应出勤：口径同看板（已出勤 + 已入职、排了班、非休息日且已到上班时间的还没来的人）
    public int Present { get; set; }      // 出勤（打了上班卡或出差）
    public int NotStarted { get; set; }   // 排了班但还没到上班时间的人（不算进应出勤）

    /// <summary>"未排班"这一行：这些人没有班次，没法判断该不该来，所以只数出勤、不给出勤率。</summary>
    public bool IsUnscheduled { get; set; }

    /// <summary>出勤率（百分比，一位小数）；未排班行或应出勤为 0 时为空。</summary>
    public double? Rate => !IsUnscheduled && Expected > 0 ? Math.Round((double)Present / Expected * 100, 1) : null;
}
