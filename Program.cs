using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Serilog;
using AttendanceSystem.Data;
using AttendanceSystem.Middlewares;
using AttendanceSystem.Models.Enums;
using AttendanceSystem.Models.Options;
using AttendanceSystem.Services.BackgroundServices;
using AttendanceSystem.Services.Implementations;
using AttendanceSystem.Services.Interfaces;

// 这是整个程序的“启动入口”：从上到下配置好各种功能，最后启动网站。
// 大致顺序：配置日志 → 注册各种服务 → 组装管线 → 运行。

// 先配一个临时日志器（启动早期就能打日志）
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

var builder = WebApplication.CreateBuilder(args);   // 创建“程序构建器”

// 用 Serilog 接管日志，配置从 appsettings.json 读
builder.Host.UseSerilog((ctx, lc) => lc.ReadFrom.Configuration(ctx.Configuration));

// ── 数据库：MariaDB（用 EF Core + Pomelo 驱动连接）─────────────────────────────
var connStr = builder.Configuration.GetConnectionString("Default")!;   // 从配置取连接串
builder.Services.AddDbContext<AttendanceDbContext>(options =>
    options.UseMySql(connStr,
        ServerVersion.AutoDetect(connStr),   // 自动识别数据库版本
        mysql =>
        {
            mysql.CommandTimeout(60);         // 单条命令最多等 60 秒
            mysql.EnableRetryOnFailure(3);    // 失败自动重试 3 次
        }));

// ── 应用配置：把 appsettings.json 的 AppSettings 节读成强类型对象 ───────────────
builder.Services.Configure<AppSettingsOptions>(
    builder.Configuration.GetSection(AppSettingsOptions.SectionName));
var appSettings = builder.Configuration.GetSection(AppSettingsOptions.SectionName)
                      .Get<AppSettingsOptions>() ?? new AppSettingsOptions();

// ── 登录认证：用 Cookie 记住登录状态 ─────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath         = "/Login";                // 没登录时跳到这
        options.LogoutPath        = "/Logout";
        options.AccessDeniedPath  = "/Login?denied=1";       // 权限不足跳到这
        options.ExpireTimeSpan    = TimeSpan.FromHours(appSettings.TokenExpireHours);  // 登录多久过期
        options.SlidingExpiration = true;                    // 有活动就自动续期

        // 网页请求没登录/无权限 → 跳转登录页（默认行为）；
        // 但 /api 接口请求不能跳转，否则调用方拿到的是 302 重定向而不是 401/403，
        // 无法正确判断“未登录”还是“无权限”（钉钉同步、报表导出等接口调用方都靠状态码判断）。
        var redirectToLogin = options.Events.OnRedirectToLogin;
        options.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }
            return redirectToLogin(ctx);
        };
        var redirectToAccessDenied = options.Events.OnRedirectToAccessDenied;
        options.Events.OnRedirectToAccessDenied = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api"))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }
            return redirectToAccessDenied(ctx);
        };
    });

// ── 授权策略：定义两种“权限门槛” ─────────────────────────────────────────────────
builder.Services.AddAuthorization(options =>
{
    // 管理类：管理员、文员
    options.AddPolicy("ManagePolicy", p =>
        p.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Clerk)));
    // 审批类：管理员、文员、主管、班组长
    options.AddPolicy("ApprovePolicy", p =>
        p.RequireRole(nameof(UserRole.Admin), nameof(UserRole.Clerk),
                      nameof(UserRole.Supervisor), nameof(UserRole.TeamLeader)));
});

// ── 注册业务服务：注册后，控制器/页面就能“拿来即用”（依赖注入）────────────────────
builder.Services.AddScoped<IUserService,           UserService>();
builder.Services.AddScoped<IAttendanceService,     AttendanceService>();
builder.Services.AddScoped<IApprovalService,       ApprovalService>();
builder.Services.AddScoped<IAttendanceGroupService, AttendanceGroupService>();
builder.Services.AddScoped<IEmployeeRegistrationService, EmployeeRegistrationService>();

// ── 钉钉对接相关注册 ────────────────────────────────────────────────────────────
builder.Services.Configure<DingTalkOptions>(
    builder.Configuration.GetSection(DingTalkOptions.SectionName));
builder.Services.AddMemoryCache();   // 用于缓存钉钉令牌
// 下面三个是带超时设置的 HTTP 客户端，分别调钉钉的“令牌/打卡/通讯录”接口
builder.Services.AddHttpClient<IDingTalkTokenProvider,    DingTalkTokenProvider>(
    c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IDingTalkAttendanceClient, DingTalkAttendanceClient>(
    c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IDingTalkContactClient,    DingTalkContactClient>(
    c => c.Timeout = TimeSpan.FromSeconds(60));
builder.Services.AddHttpClient<IDingTalkLeaveClient,      DingTalkLeaveClient>(
    c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<IDingTalkNotifyClient,     DingTalkNotifyClient>(
    c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<IDingTalkSyncService,          DingTalkSyncService>();
builder.Services.AddScoped<IPasswordResetService,         PasswordResetService>();   // 忘记密码找回（钉钉工作通知验证码）

// ── 网页(Razor Pages) + 接口(Web API)──────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // 接口返回的 JSON：字段名用小驼峰(camelCase)，并忽略值为 null 的字段
        o.JsonSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase;
        o.JsonSerializerOptions.DefaultIgnoreCondition =
            System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    });

builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(o =>           // 会话（临时存一些用户相关数据）
{
    o.IdleTimeout     = TimeSpan.FromMinutes(30);
    o.Cookie.HttpOnly = true;
});
builder.Services.AddHttpContextAccessor();

// 后台定时任务（旷工标记 + 月度汇总 + 可选钉钉同步）
builder.Services.AddHostedService<AttendanceBackgroundService>();

// 文件上传限制：整个上传请求体上限 50MB（约相当于 5 个 10MB 附件）
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o =>
{
    o.MultipartBodyLengthLimit = 50 * 1024 * 1024;
});

// ── 以上是“注册阶段”，下面 Build 之后进入“运行阶段”────────────────────────────────
var app = builder.Build();

// 开发环境：启动时自动建表/更新表结构（迁移）+ 播种默认管理员
if (app.Environment.IsDevelopment())
{
    using var scope = app.Services.CreateScope();
    var dbCtx = scope.ServiceProvider.GetRequiredService<AttendanceDbContext>();
    try
    {
        await dbCtx.Database.MigrateAsync();   // 自动迁移
        Log.Information("数据库迁移完成");
        await SeedAdminAsync(dbCtx);
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "数据库迁移失败，请检查连接字符串（appsettings.json）和 MariaDB 服务");
    }
}

// 播种：如果库里一个用户都没有，就创建默认部门/考勤组和管理员账号 admin / Admin@123
static async Task SeedAdminAsync(AttendanceDbContext db)
{
    if (await db.Users.AnyAsync()) return;   // 已有用户就不播种

    var dept = await db.Departments.FirstOrDefaultAsync()
               ?? new AttendanceSystem.Models.Entities.Department
                  { DeptName = "总公司", DeptCode = "HQ", IsActive = true, CreatedAt = DateTime.Now };
    if (dept.Id == 0) { db.Departments.Add(dept); await db.SaveChangesAsync(); }

    var group = await db.AttendanceGroups.FirstOrDefaultAsync()
                ?? new AttendanceSystem.Models.Entities.AttendanceGroup
                   { GroupName = "总公司考勤组", IsActive = true, CreatedAt = DateTime.Now };
    if (group.Id == 0) { db.AttendanceGroups.Add(group); await db.SaveChangesAsync(); }

    db.Users.Add(new AttendanceSystem.Models.Entities.User
    {
        EmployeeNo        = "admin",
        RealName          = "系统管理员",
        PasswordHash      = AttendanceSystem.Services.Implementations.UserService.HashPassword("Admin@123"),
        Role              = AttendanceSystem.Models.Enums.UserRole.Admin,
        DepartmentId      = dept.Id,
        AttendanceGroupId = group.Id,
        IsActive          = true,
        CreatedAt         = DateTime.Now,
        UpdatedAt         = DateTime.Now
    });
    await db.SaveChangesAsync();
    Log.Information("已创建默认管理员账号：admin / Admin@123");
}

app.UseSerilogRequestLogging();   // 记录每个请求的日志

// 全局异常兜底（开发/生产环境都注册，之前只在生产环境注册，导致开发环境未捕获异常时是一片空白，不好排查）：
// ① 已知的“业务校验”异常（InvalidOperationException/KeyNotFoundException，服务层专门用来抛用户能看懂的提示语，
//    例如“工号已存在”“账号已停用”）→ 翻译成对应的 400/404 状态码 + 原始提示语，而不是笼统的 500；
//    这类异常此前只有 Razor 页面自己 try/catch 了，Web API 控制器（登录、建员工等）没接住，会直接变成 500。
// ② 其余未预期的异常 → 生产环境回统一友好文案，开发环境回异常详情方便排查。
// 接口请求(/api)回 JSON，网页请求回 HTML。
app.UseExceptionHandler(errApp => errApp.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var isDev = app.Environment.IsDevelopment();

    var (status, message) = error switch
    {
        InvalidOperationException => (StatusCodes.Status400BadRequest, error.Message),
        KeyNotFoundException      => (StatusCodes.Status404NotFound, error.Message),
        _ => (StatusCodes.Status500InternalServerError,
              isDev && error is not null ? error.ToString() : "服务器内部错误，请稍后重试或联系管理员")
    };

    context.Response.StatusCode = status;
    if (context.Request.Path.StartsWithSegments("/api"))   // 接口请求
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new { success = false, message });
    }
    else   // 网页请求
    {
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(
            "<meta charset=\"utf-8\"><div style=\"text-align:center;margin-top:80px;font-family:'微软雅黑'\">" +
            $"<h2>系统出错了</h2><p style=\"color:#888;white-space:pre-wrap;text-align:left;max-width:900px;margin:0 auto\">{System.Net.WebUtility.HtmlEncode(isDev ? message : "请稍后重试，或联系管理员。")}</p>" +
            "<p><a href=\"/\">返回首页</a></p></div>");
    }
}));

// ── 请求处理管线：每个请求按下面顺序依次经过这些“关卡”────────────────────────────
app.UseStaticFiles();                          // 静态文件(css/js/图片)
app.UseRouting();                              // 路由（决定请求交给谁处理）
app.UseSession();                              // 会话
app.UseAuthentication();                       // 认证（你是谁）
app.UseAuthorization();                        // 授权（你有没有权限）
app.UseMiddleware<CurrentUserMiddleware>();    // 自定义关卡：停用账号踢下线 + 记录当前用户

app.MapRazorPages();      // 网页
app.MapControllers();     // 接口

// 访问根路径 "/" 时，直接跳到登录页
app.MapGet("/", ctx =>
{
    ctx.Response.Redirect("/Login");
    return Task.CompletedTask;
});

await app.RunAsync();   // 启动网站，开始接收请求
