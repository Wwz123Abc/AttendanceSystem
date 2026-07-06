using Microsoft.EntityFrameworkCore;
using AttendanceSystem.Models.Entities;
using AttendanceSystem.Models.Enums;

namespace AttendanceSystem.Data;

// 「DbContext」= 程序和数据库之间的“桥梁”。
// 通过它，程序就能用 C# 代码来增删改查数据库，而不用手写 SQL。

/// <summary>
/// EF Core 数据上下文。
/// ● 表名、列名都用 PascalCase（即直接用 C# 类名/属性名，如 User、EmployeeNo）；
/// ● 在这里集中配置各表之间的关系、索引、约束和初始数据。
/// </summary>
public class AttendanceDbContext : DbContext
{
    // 构造函数：接收数据库连接配置（连哪个库、用什么账号等，在 Program.cs 里传进来）
    public AttendanceDbContext(DbContextOptions<AttendanceDbContext> options) : base(options) { }

    // ── DbSet：每个 DbSet 就是一张表的“操作入口”，下面对应数据库里的 13 张表 ──
    public DbSet<User>                      Users                      => Set<User>();
    public DbSet<Department>                Departments                => Set<Department>();
    public DbSet<AttendanceGroup>           AttendanceGroups           => Set<AttendanceGroup>();
    public DbSet<ShiftSchedule>             ShiftSchedules             => Set<ShiftSchedule>();
    public DbSet<ShiftAssignment>           ShiftAssignments           => Set<ShiftAssignment>();
    public DbSet<AttendancePunch>           AttendancePunches          => Set<AttendancePunch>();
    public DbSet<AttendanceRecord>          AttendanceRecords          => Set<AttendanceRecord>();
    public DbSet<MonthlyAttendanceSummary>  MonthlyAttendanceSummaries => Set<MonthlyAttendanceSummary>();
    public DbSet<ApprovalRequest>           ApprovalRequests           => Set<ApprovalRequest>();
    public DbSet<ApprovalStep>              ApprovalSteps              => Set<ApprovalStep>();
    public DbSet<ApprovalFlow>              ApprovalFlows              => Set<ApprovalFlow>();
    public DbSet<Holiday>                   Holidays                   => Set<Holiday>();
    public DbSet<Notification>              Notifications              => Set<Notification>();

    // 这个方法在“建立数据库模型”时被调用，用来配置表名、关系、索引、唯一约束等。
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ── 统一命名：强制表名/列名保持和 C# 一致（PascalCase）──────────────
        // 防止 MySQL/Pomelo 自动把名字改成小写，遍历每张表逐一指定名字。
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            entityType.SetTableName(entityType.ClrType.Name);            // 表名 = 类名

            foreach (var property in entityType.GetProperties())
                property.SetColumnName(property.Name);                   // 列名 = 属性名

            foreach (var key in entityType.GetKeys())
                key.SetName($"PK_{entityType.ClrType.Name}");            // 主键名统一为 PK_表名

            foreach (var fk in entityType.GetForeignKeys())              // 外键名统一为 FK_本表_主表_列
                fk.SetConstraintName(
                    $"FK_{entityType.ClrType.Name}_{fk.PrincipalEntityType.ClrType.Name}_{string.Join("_", fk.Properties.Select(p => p.Name))}");

            foreach (var idx in entityType.GetIndexes())                 // 索引名统一为 IX_表名_列
                idx.SetDatabaseName(
                    $"IX_{entityType.ClrType.Name}_{string.Join("_", idx.Properties.Select(p => p.Name))}");
        }

        // ── User：工号唯一、手机号建索引；部门/考勤组/上级删除时把外键置空 ──
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.EmployeeNo).IsUnique();   // 工号不能重复
            e.HasIndex(u => u.Phone);                   // 给手机号建索引，查得更快

            // 上级被删 → 下属的“上级”字段清空（SetNull），不连带删下属
            e.HasOne(u => u.Supervisor)
             .WithMany()
             .HasForeignKey(u => u.SupervisorUserId)
             .OnDelete(DeleteBehavior.SetNull);

            // 部门被删 → 员工的“部门”字段清空，员工本身保留
            e.HasOne(u => u.Department)
             .WithMany(d => d.Users)
             .HasForeignKey(u => u.DepartmentId)
             .OnDelete(DeleteBehavior.SetNull);

            // 考勤组被删 → 员工的“考勤组”字段清空，员工本身保留
            e.HasOne(u => u.AttendanceGroup)
             .WithMany(g => g.Users)
             .HasForeignKey(u => u.AttendanceGroupId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── Department：部门的父子关系（上级部门删除时把子部门的 ParentId 置空）──
        modelBuilder.Entity<Department>(e =>
        {
            e.HasOne(d => d.ParentDepartment)
             .WithMany(d => d.ChildDepartments)
             .HasForeignKey(d => d.ParentId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        // ── AttendanceRecord：同一人同一天只能有一条；员工删了，其考勤记录一起删 ──
        modelBuilder.Entity<AttendanceRecord>(e =>
        {
            e.HasIndex(r => new { r.UserId, r.WorkDate }).IsUnique();   // (员工,日期) 唯一

            e.HasOne(r => r.User)
             .WithMany(u => u.AttendanceRecords)
             .HasForeignKey(r => r.UserId)
             .OnDelete(DeleteBehavior.Cascade);                        // 连带删除
        });

        // ── AttendancePunch：按(员工,打卡时间)建索引；员工删了，打卡流水一起删 ──
        modelBuilder.Entity<AttendancePunch>(e =>
        {
            e.HasIndex(p => new { p.UserId, p.PunchTime });

            e.HasOne(p => p.User)
             .WithMany()
             .HasForeignKey(p => p.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── MonthlyAttendanceSummary：同一人同一年月只能有一条 ──
        modelBuilder.Entity<MonthlyAttendanceSummary>(e =>
        {
            e.HasIndex(s => new { s.UserId, s.Year, s.Month }).IsUnique();
        });

        // ── ShiftAssignment：同一人同一天只能排一个班 ──
        modelBuilder.Entity<ShiftAssignment>(e =>
        {
            e.HasIndex(a => new { a.UserId, a.WorkDate }).IsUnique();
        });

        // ── ApprovalRequest：单号唯一；按(申请人,状态)建索引；申请人删了，申请一起删 ──
        modelBuilder.Entity<ApprovalRequest>(e =>
        {
            e.HasIndex(a => a.RequestNo).IsUnique();
            e.HasIndex(a => new { a.ApplicantUserId, a.ApprovalStatus });

            e.HasOne(a => a.Applicant)
             .WithMany(u => u.ApprovalRequests)
             .HasForeignKey(a => a.ApplicantUserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── ApprovalStep：申请单删了，其审批节点一起删；但审批人用 Restrict（不允许直接删审批人，避免历史丢失）──
        modelBuilder.Entity<ApprovalStep>(e =>
        {
            e.HasOne(s => s.ApprovalRequest)
             .WithMany(a => a.ApprovalSteps)
             .HasForeignKey(s => s.ApprovalRequestId)
             .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(s => s.Approver)
             .WithMany()
             .HasForeignKey(s => s.ApproverUserId)
             .OnDelete(DeleteBehavior.Restrict);
        });

        // ── ApprovalFlow：考勤组删了，其审批流配置一起删 ──
        modelBuilder.Entity<ApprovalFlow>(e =>
        {
            e.HasOne(f => f.AttendanceGroup)
             .WithMany(g => g.ApprovalFlows)
             .HasForeignKey(f => f.AttendanceGroupId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── Holiday：按(日期,考勤组)建索引，查某天是否假期更快 ──
        modelBuilder.Entity<Holiday>(e =>
        {
            e.HasIndex(h => new { h.HolidayDate, h.AttendanceGroupId });
        });

        // ── Notification：员工删了，其通知一起删 ──
        modelBuilder.Entity<Notification>(e =>
        {
            e.HasOne(n => n.User)
             .WithMany(u => u.Notifications)
             .HasForeignKey(n => n.UserId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        // ── 写入初始“种子数据”（首次建库时自动插入）──
        SeedInitialData(modelBuilder);
    }

    // 初始种子数据：新库建好时，自动放入一个默认部门、一个默认考勤组、一个默认班次。
    private static void SeedInitialData(ModelBuilder modelBuilder)
    {
        var now = new DateTime(2024, 1, 1);  // 固定时间，保证每次生成的迁移一致

        // 默认部门：总公司
        modelBuilder.Entity<Department>().HasData(new Department
        {
            Id          = 1,
            DeptName    = "总公司",
            DeptCode    = "HQ",
            CompanyName = "总公司",
            IsActive    = true,
            CreatedAt   = now,
            UpdatedAt   = now
        });

        // 默认考勤组：总公司考勤组
        modelBuilder.Entity<AttendanceGroup>().HasData(new AttendanceGroup
        {
            Id                 = 1,
            GroupName          = "总公司考勤组",
            CompanyName        = "总公司",
            PunchRadiusMeters  = 500,
            EnableLocationPunch = false,
            LunchBreakMinutes  = 60,
            DinnerBreakMinutes = 30,
            IsActive           = true,
            CreatedAt          = now,
            UpdatedAt          = now
        });

        // 默认班次：正常班 9:00–18:00
        modelBuilder.Entity<ShiftSchedule>().HasData(new ShiftSchedule
        {
            Id                        = 1,
            AttendanceGroupId         = 1,
            ShiftName                 = "正常班",
            ShiftType                 = ShiftType.Fixed,
            WorkStartTime             = new TimeOnly(9, 0),
            WorkEndTime               = new TimeOnly(18, 0),
            LateToleranceMinutes      = 5,
            EarlyLeaveToleranceMinutes = 5,
            EarliestClockInMinutes    = 60,
            OvertimeThresholdMinutes  = 30,
            IsCrossDay                = false,
            StandardWorkHours         = 8,
            Color                     = "#1890ff",
            IsActive                  = true,
            CreatedAt                 = now,
            UpdatedAt                 = now
        });
    }
}
