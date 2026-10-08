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
    public DbSet<AttendanceGroupApprover>   AttendanceGroupApprovers   => Set<AttendanceGroupApprover>();
    public DbSet<AttendanceGroupLocation>   AttendanceGroupLocations   => Set<AttendanceGroupLocation>();
    public DbSet<Holiday>                   Holidays                   => Set<Holiday>();
    public DbSet<Notification>              Notifications              => Set<Notification>();
    public DbSet<EmployeeRegistration>      EmployeeRegistrations      => Set<EmployeeRegistration>();
    public DbSet<Announcement>              Announcements              => Set<Announcement>();
    public DbSet<AnnouncementRead>          AnnouncementReads          => Set<AnnouncementRead>();
    public DbSet<ZKDeviceCommand>           ZKDeviceCommands           => Set<ZKDeviceCommand>();
    public DbSet<ZKDevice>                  ZKDevices                  => Set<ZKDevice>();
    public DbSet<FaceVerifyAttempt>         FaceVerifyAttempts         => Set<FaceVerifyAttempt>();
    public DbSet<UserZKDevice>              UserZKDevices              => Set<UserZKDevice>();
    public DbSet<AgentConversation>         AgentConversations         => Set<AgentConversation>();
    public DbSet<AgentMessage>              AgentMessages              => Set<AgentMessage>();
    public DbSet<AgentPendingAction>        AgentPendingActions        => Set<AgentPendingAction>();
    public DbSet<AgentActionLog>            AgentActionLogs            => Set<AgentActionLog>();

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

        // ── 各表的关系、索引、唯一约束：每个实体一个配置类，放在 Data/Configurations 下 ──
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AttendanceDbContext).Assembly);

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
