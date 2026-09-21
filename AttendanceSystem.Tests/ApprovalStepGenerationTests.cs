using AttendanceSystem.Data;
using AttendanceSystem.Models.DTOs;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.Implementations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace AttendanceSystem.Tests;

/// <summary>
/// 回归测试：二级审批考勤组，如果没配"审批人名单"，一级节点会退回"直属上级"；这种组下再自动追加
/// 二级节点时，以前直接拿同一个"直属上级 ?? 兜底"表达式再算一遍，跟一级是同一个人，等于要同一个人
/// 对同一张单连点两次"通过"（2026-09-21 代码审查发现，见 docs/项目审查与问题总表.md B10）。
/// </summary>
public class ApprovalStepGenerationTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private const int ApplicantId  = 1;
    private const int SupervisorId = 2;
    private static readonly IOptions<AppSettingsOptions> AppOptions = Options.Create(new AppSettingsOptions());

    public ApprovalStepGenerationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private AttendanceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AttendanceDbContext>().UseSqlite(_connection).Options;
        return new AttendanceDbContext(options);
    }

    [Fact]
    public async Task 二级审批组没配审批人名单_一级退回直属上级时_不会生成重复的二级节点()
    {
        using (var db = CreateContext())
        {
            db.AttendanceGroups.Add(new AttendanceGroup { Id = 999, GroupName = "二级审批组", ApprovalLevel = ApprovalLevelType.Level2 });
            db.Users.Add(new User { Id = SupervisorId, EmployeeNo = "E002", RealName = "直属上级", PasswordHash = "x" });
            db.Users.Add(new User
            {
                Id = ApplicantId, EmployeeNo = "E001", RealName = "申请人", PasswordHash = "x",
                AttendanceGroupId = 999, SupervisorUserId = SupervisorId
            });
            db.SaveChanges();
        }

        using var db2 = CreateContext();
        var svc = new ApprovalService(db2, new FakeAttendanceService(), AppOptions);
        var dto = new SubmitApprovalDto
        {
            ApprovalType   = ApprovalType.Leave,
            Reason         = "测试",
            LeaveStartTime = DateTime.Today.AddHours(9),
            LeaveEndTime   = DateTime.Today.AddHours(12)
        };
        var request = await svc.SubmitApprovalAsync(ApplicantId, dto);

        using var verify = CreateContext();
        var steps = await verify.ApprovalSteps.Where(s => s.ApprovalRequestId == request.Id).ToListAsync();

        // 旧逻辑会生成两个节点，StepOrder=1 和 2，两个 ApproverUserId 都是 SupervisorId——
        // 现在只应该有一个节点，一级通过就是整单通过。
        Assert.Single(steps);
        Assert.Equal(SupervisorId, steps[0].ApproverUserId);
        Assert.Equal(1, steps[0].StepOrder);
    }
}
