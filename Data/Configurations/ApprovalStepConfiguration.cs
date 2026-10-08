using AttendanceSystem.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AttendanceSystem.Data.Configurations;

/// <summary>
/// ApprovalStep：申请单删了，其审批节点一起删；但审批人用 Restrict（不允许直接删审批人，避免历史丢失）
/// </summary>
public class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> e)
    {
        // "待我审批/已处理"这类按(审批人,状态)过滤的查询在审批人的仪表盘每次打开都会跑一遍，
        // 之前只有 ApproverUserId 外键带的单列索引，跟 ApprovalRequest 已经有的
        // (ApplicantUserId, ApprovalStatus) 组合索引不对称（发现于 2026-09-21 数据核查）。
        e.HasIndex(s => new { s.ApproverUserId, s.ApprovalStatus });

        e.HasOne(s => s.ApprovalRequest)
         .WithMany(a => a.ApprovalSteps)
         .HasForeignKey(s => s.ApprovalRequestId)
         .OnDelete(DeleteBehavior.Cascade);

        e.HasOne(s => s.Approver)
         .WithMany()
         .HasForeignKey(s => s.ApproverUserId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
