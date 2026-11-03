namespace AttendanceSystem.Helpers;

/// <summary>
/// 考勤核对（员工自助核对 + 异议反馈）功能开关。
/// 2026-11-03 下午用户要求"先下线"：菜单入口隐藏、页面直接 404、提交/处理接口一并关掉。
/// 代码、数据表（AttendanceDispute）、历史异议数据都保留 —— 把 <see cref="Enabled"/> 改回 true
/// 重新发布一次即可恢复，不需要迁移数据库。
/// </summary>
public static class AttendanceDisputeFeature
{
    /// <summary>是否启用考勤核对功能。</summary>
    public const bool Enabled = false;
}
